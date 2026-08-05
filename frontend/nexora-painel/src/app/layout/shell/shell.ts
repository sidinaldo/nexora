import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { OnboardingServico } from '../../nucleo/servicos/onboarding.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ToastPilha } from '../../nucleo/toast/toast';
import { StatusPainel } from '../../nucleo/modelos';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastPilha],
  templateUrl: './shell.html',
  styleUrl: './shell.css'
})
export class Shell implements OnInit, OnDestroy {
  auth = inject(AuthServico);
  realtime = inject(RealtimeServico);
  onboarding = inject(OnboardingServico);
  private painel = inject(PainelServico);
  private toast = inject(ToastServico);

  status = signal<StatusPainel | null>(null);

  /** Começa true para não piscar o banner de "WhatsApp desconectado" antes do 1º carregamento. */
  whatsappConectado = signal(true);
  naoLidas = signal(0);

  private timer: ReturnType<typeof setInterval> | null = null;
  private assinaturas: { unsubscribe(): void }[] = [];

  async ngOnInit() {
    await this.realtime.conectar();
    this.carregarStatus();

    // O link "Primeiros passos" só aparece enquanto falta passo. Uma leitura no boot basta: o
    // estado muda por ação do usuário (conectar, convidar) e a tela de primeiros passos
    // recarrega sozinha. Falhar aqui não pode derrubar o shell.
    this.onboarding.carregar().subscribe({ error: () => { } });

    this.assinaturas.push(
      // Mensagem chegando pelo celular do cliente: badge sobe e o toast avisa, mesmo que o
      // vendedor esteja em outra tela.
      this.realtime.mensagemRecebida$.subscribe(m => {
        if (m.direcao === 'entrada') {
          this.naoLidas.update(n => n + 1);
          this.toast.info(`${m.contatoNome}: ${m.previa ?? 'nova mensagem'}`);
        }
      }),
      this.realtime.contatoCriado$.subscribe(c =>
        this.toast.sucesso(`Novo lead pelo WhatsApp: ${c.nome}`)),
      // A queda do número é o aviso mais importante do painel: sem ele o vendedor digita uma
      // resposta que não vai sair.
      this.realtime.conexaoMudou$.subscribe(c => {
        const conectado = c.status === 'conectado';
        this.whatsappConectado.set(conectado);
        if (!conectado) this.toast.erro('O WhatsApp desconectou. Reconecte em Conexão.');
      })
    );

    // O status também chega por webhook, mas um poll leve mantém badge e banner frescos em
    // qualquer tela — inclusive se o hub cair. 45s basta: não é evento de segundo a segundo.
    this.timer = setInterval(() => this.carregarStatus(), 45_000);
  }

  ngOnDestroy() {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
    this.assinaturas.forEach(a => a.unsubscribe());
    this.realtime.desconectar();
  }

  private carregarStatus() {
    this.painel.status().subscribe({
      next: s => {
        this.status.set(s);
        this.naoLidas.set(s.naoLidas);
        this.whatsappConectado.set(s.whatsappConectado);
      },
      // O status não pode derrubar a tela inteira.
      error: () => { }
    });
  }

  iniciais(nome: string | undefined): string {
    if (!nome) return '?';
    const p = nome.trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase();
  }
}
