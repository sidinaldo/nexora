import {
  Component, ElementRef, OnDestroy, ViewChild, effect, inject, input, output, signal, untracked
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { Subscription } from 'rxjs';
import { CaixaServico } from '../servicos/caixa.servico';
import { RealtimeServico } from '../servicos/realtime.servico';
import { ToastServico } from '../toast/toast.servico';
import { MensagemDto } from '../modelos';
import { TickStatus, estadoDoAck, rotuloAck } from '../tick-status/tick-status';

/** A THREAD DA CONVERSA — mensagens, rolagem e envio.
 *
 *  ===================== POR QUE ISTO É UM COMPONENTE =====================
 *  Duas telas mostram a mesma conversa: a caixa de entrada e o detalhe do contato. A mecânica
 *  aqui NÃO é trivial e é justamente a que se erra ao duplicar:
 *
 *  - paginação por CURSOR para trás (as mais novas antes de um id), nunca por offset;
 *  - ÂNCORA DE ROLAGEM ao carregar as anteriores: compensa o scrollTop pela altura inserida no
 *    topo, senão a thread "pula" na cara de quem está lendo;
 *  - rolagem 'auto' quando chega mensagem: rola só se o vendedor JÁ estava no fim; se ele
 *    subiu para ler algo, mostra o chip "nova mensagem" e NÃO rouba a rolagem.
 *
 *  Duplicar isso significa consertar cada bug duas vezes — e descobrir o segundo bug meses
 *  depois, na tela que ninguém testou.
 *  ========================================================================
 *
 *  O CABEÇALHO fica fora: a caixa mostra assumir/liberar, o detalhe do contato mostra outra
 *  coisa. O que é comum é a thread, não o que está em volta dela. */
@Component({
  selector: 'app-thread',
  imports: [FormsModule, DatePipe, TickStatus],
  templateUrl: './thread.html',
  styleUrl: './thread.css'
})
export class Thread implements OnDestroy {
  private servico = inject(CaixaServico);
  private realtime = inject(RealtimeServico);
  private toast = inject(ToastServico);

  /** A conversa a mostrar. Trocar o valor recarrega tudo. */
  conversaId = input.required<number>();

  /** Quantas não lidas a conversa tinha ao abrir — dispara o "marcar lida". */
  naoLidas = input(0);

  /** Algo mudou que a tela de fora precisa saber: mensagem enviada ou conversa marcada como
   *  lida. A lista da caixa se reordena com isso; o detalhe do contato recarrega o cabeçalho. */
  mudou = output<void>();

  mensagens = signal<MensagemDto[]>([]);
  temMaisAntigas = signal(false);
  carregandoAntigas = signal(false);
  carregando = signal(true);
  /** Chip "↓ Nova mensagem": aparece quando chega mensagem e o vendedor rolou para cima. */
  temNovaMensagem = signal(false);

  texto = signal('');
  enviando = signal(false);

  @ViewChild('thread') private threadEl?: ElementRef<HTMLDivElement>;

  private inscricoes: Subscription[] = [];

  constructor() {
    // O `untracked` é necessário: sem ele, os signals escritos dentro de `abrir` (mensagens,
    // carregando…) entrariam nas dependências do effect e ele reexecutaria em laço.
    effect(() => {
      const id = this.conversaId();
      untracked(() => this.abrir(id));
    });

    this.inscricoes.push(
      this.realtime.mensagemRecebida$.subscribe(m => {
        if (m.conversaId === this.conversaId()) this.recarregar('auto');
      }),
      // ACK só muda o tick: não pode mexer na posição de leitura.
      this.realtime.statusMensagem$.subscribe(() => this.recarregar('preservar'))
    );
  }

  ngOnDestroy() { this.inscricoes.forEach(s => s.unsubscribe()); }

  private abrir(conversaId: number) {
    this.texto.set('');
    this.temNovaMensagem.set(false);
    this.carregando.set(true);
    this.mensagens.set([]);

    this.servico.mensagens(conversaId).subscribe({
      next: p => {
        this.mensagens.set(p.itens);
        this.temMaisAntigas.set(p.temMais);
        this.carregando.set(false);
        this.aposRender(() => this.rolarParaFim(false));
      },
      error: () => this.carregando.set(false)
    });

    if (this.naoLidas() > 0) {
      this.servico.marcarLida(conversaId).subscribe(() => this.mudou.emit());
    }
  }

  /** 'fim' = rola para o fim (acabou de enviar) · 'auto' = rola só se já estava no fim ·
   *  'preservar' = não mexe na rolagem. */
  private recarregar(modo: 'fim' | 'auto' | 'preservar') {
    const id = this.conversaId();
    const jaNoFim = this.estaNoFim();
    // +1 para incluir a nova sem descartar o topo que o vendedor está lendo.
    const n = Math.max(30, this.mensagens().length + 1);

    this.servico.mensagens(id, undefined, n).subscribe(p => {
      this.mensagens.set(p.itens);
      this.temMaisAntigas.set(p.temMais);
      if (modo === 'fim') this.aposRender(() => this.rolarParaFim(true));
      else if (modo === 'auto') {
        if (jaNoFim) this.aposRender(() => this.rolarParaFim(true));
        else this.temNovaMensagem.set(true);
      }
    });
  }

  /** Prepende PRESERVANDO a posição de leitura: compensa o scrollTop pela altura inserida no
   *  topo. Sem essa compensação a thread pula e o vendedor perde o que estava lendo. */
  carregarAntigas() {
    const primeira = this.mensagens()[0];
    if (!primeira || this.carregandoAntigas()) return;

    this.carregandoAntigas.set(true);
    const el = this.threadEl?.nativeElement;
    const alturaAntes = el?.scrollHeight ?? 0;
    const topoAntes = el?.scrollTop ?? 0;

    this.servico.mensagens(this.conversaId(), primeira.id).subscribe({
      next: p => {
        this.mensagens.update(atual => [...p.itens, ...atual]);
        this.temMaisAntigas.set(p.temMais);
        this.carregandoAntigas.set(false);
        this.aposRender(() => {
          const e2 = this.threadEl?.nativeElement;
          if (e2) e2.scrollTop = topoAntes + (e2.scrollHeight - alturaAntes);
        });
      },
      error: () => this.carregandoAntigas.set(false)
    });
  }

  enviar() {
    const t = this.texto().trim();
    if (!t || this.enviando()) return;

    this.enviando.set(true);
    this.servico.responder(this.conversaId(), t).subscribe({
      next: r => {
        this.texto.set('');
        this.enviando.set(false);
        this.recarregar('fim');
        this.mudou.emit();
        if (!r.enviada) {
          // A mensagem EXISTE e aparece na thread marcada como "não chegou" — não é erro de
          // requisição, é falha de entrega. O toast diz isso sem bloquear.
          this.toast.erro(r.erro ?? 'A mensagem foi registrada mas não chegou ao WhatsApp.');
        }
      },
      error: e => {
        this.enviando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível enviar.');
      }
    });
  }

  tick(m: MensagemDto) { return estadoDoAck(m.ack, m.enviadaEm, m.erro); }
  tickTitulo(m: MensagemDto) { return m.erro ?? rotuloAck(m.ack); }

  irParaFim() { this.rolarParaFim(true); }

  // ---------------------------------------------------------------- rolagem
  private aposRender(fn: () => void) { setTimeout(fn, 0); }

  private prefereReduzirMovimento(): boolean {
    return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  }

  private rolarParaFim(suave: boolean) {
    const el = this.threadEl?.nativeElement;
    if (!el) return;
    this.temNovaMensagem.set(false);
    el.scrollTo({
      top: el.scrollHeight,
      behavior: suave && !this.prefereReduzirMovimento() ? 'smooth' : 'auto'
    });
  }

  /** O vendedor está no fim (ou a ~150px dele)? Sem container ainda = tratar como fim. */
  private estaNoFim(margem = 150): boolean {
    const el = this.threadEl?.nativeElement;
    if (!el) return true;
    return el.scrollHeight - el.scrollTop - el.clientHeight <= margem;
  }
}
