import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { ConversoesServico } from '../../../nucleo/servicos/conversoes.servico';
import { ToastServico } from '../../../nucleo/toast/toast.servico';
import { ConversaoDto, CredencialDto, ResultadoTesteConversao } from '../../../nucleo/modelos';

/** ANÚNCIOS — o painel da aba "Anúncios" em `/integracoes` (INT-4).
 *
 *  ===================== O QUE ESTA ABA RESOLVE =====================
 *  O dono anuncia no Instagram, a pessoa clica, vira lead, e compra três dias depois. A Meta nunca
 *  fica sabendo que aquele clique virou venda — então o algoritmo dela continua procurando quem
 *  preenche formulário, não quem compra.
 *
 *  Conectar o pixel e o token é o que fecha esse laço. É a única coisa que se faz aqui.
 *  =================================================================
 *
 *  ===================== O TOKEN NÃO VOLTA DA API, NUNCA =====================
 *  O campo nasce VAZIO mesmo quando já existe token salvo, e vazio significa "não mexi nisso".
 *  A tela mostra o sufixo (`EAAG…4Zc`) para a pessoa reconhecer qual está lá.
 *
 *  Não é teatro: um token que a tela busca a cada carregamento é um token que vive no histórico do
 *  navegador, no cache do proxy e na captura de tela do suporte.
 *  ==========================================================================
 *
 *  ===================== O CONSENTIMENTO É UM BLOCO PRÓPRIO =====================
 *  Não é uma caixinha no meio das outras. Marcá-la é DECLARAR que o site tem base legal para
 *  mandar dado de visitante para a Meta — e quem declara fica registrado, com data.
 *  ============================================================================== */
@Component({
  selector: 'app-integracoes-anuncios',
  imports: [FormsModule, CurrencyPipe, DatePipe],
  templateUrl: './anuncios.html',
  styleUrl: './anuncios.css'
})
export class IntegracaoAnuncios implements OnInit {
  private servico = inject(ConversoesServico);
  private toast = inject(ToastServico);

  credencial = signal<CredencialDto | null>(null);
  leadsComAnuncio = signal(0);
  conversoes = signal<ConversaoDto[]>([]);
  carregando = signal(true);
  erro = signal('');

  // ---- formulário
  fPixel = signal('');
  /** Sempre vazio ao carregar: a API não devolve o token, e vazio quer dizer "mantém o atual". */
  fToken = signal('');
  fCodigoTeste = signal('');
  fAtivo = signal(true);
  fEmLead = signal(true);
  fEmCompra = signal(true);
  fConsentimento = signal(false);

  salvando = signal(false);
  erroForm = signal('');

  testando = signal(false);
  resultadoTeste = signal<ResultadoTesteConversao | null>(null);

  /** Qual conversão está com o payload aberto. Mesmo gesto do registro de webhooks. */
  abertaId = signal<number | null>(null);

  configurado = computed(() => this.credencial() !== null);

  /** A frase que cobra. Ela só faz sentido com número: "conecte seus anúncios" é conselho,
   *  "12 leads vieram de anúncio e a Meta não ficou sabendo" é fato. */
  perdendo = computed(() => !this.credencial()?.enviando && this.leadsComAnuncio() > 0);

  /** Quantas desistiram de vez. É o número que o dono precisa ver sem procurar. */
  falhas = computed(() => this.conversoes().filter(c => c.status === 'falhou').length);

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.erro.set('');

    this.servico.obter().subscribe({
      next: p => {
        this.credencial.set(p.credencial);
        this.leadsComAnuncio.set(p.leadsComAnuncio30Dias);
        this.conversoes.set(p.conversoes);
        this.preencher(p.credencial);
        this.carregando.set(false);
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar.');
        this.carregando.set(false);
      }
    });
  }

  private preencher(c: CredencialDto | null) {
    this.fPixel.set(c?.identificador ?? '');
    this.fCodigoTeste.set(c?.codigoTeste ?? '');
    this.fAtivo.set(c?.ativo ?? true);
    this.fEmLead.set(c?.emLead ?? true);
    this.fEmCompra.set(c?.emCompra ?? true);
    this.fConsentimento.set(c?.consentimentoEm !== null && c?.consentimentoEm !== undefined);

    // ⚠️ O TOKEN VOLTA A VAZIO A CADA CARREGAMENTO. Não há o que preencher — a API não o devolve.
    this.fToken.set('');
  }

  salvar() {
    if (this.salvando()) return;

    this.erroForm.set('');
    this.salvando.set(true);

    this.servico.salvar({
      identificador: this.fPixel().trim(),
      token: this.fToken().trim() || null,
      codigoTeste: this.fCodigoTeste().trim() || null,
      ativo: this.fAtivo(),
      emLead: this.fEmLead(),
      emCompra: this.fEmCompra(),
      consentimentoDeclarado: this.fConsentimento()
    }).subscribe({
      next: () => {
        this.salvando.set(false);
        this.toast.sucesso('Anúncios conectados.');
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.erroForm.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  /** ⚠️ O BOTÃO QUE RESOLVE A MAIOR PARTE DOS CHAMADOS. Sem ele, descobrir que o token está errado
   *  exige fechar uma venda de verdade e esperar — e quando o cliente percebe que nada chegou, já
   *  passaram semanas de campanha otimizando errado.
   *
   *  Ele NÃO grava nada na fila: o evento é sintético. E não é bloqueado pelo consentimento, porque
   *  não há pessoa nenhuma nele — exigi-lo faria a ordem de configuração ser "declare que tem
   *  autorização, depois descubra se o token funciona", que é a ordem errada. */
  testar() {
    if (this.testando()) return;

    this.testando.set(true);
    this.resultadoTeste.set(null);

    this.servico.testar().subscribe({
      next: r => {
        this.testando.set(false);
        this.resultadoTeste.set(r);
        // Recarrega porque o teste pode ter DESLIGADO (ou religado) a credencial — e o selo no topo
        // tem de mudar junto.
        this.carregar();
      },
      error: e => {
        this.testando.set(false);
        this.resultadoTeste.set({
          ok: false, codigo: null, fbtraceId: null,
          erro: e.error?.erro ?? 'Não foi possível testar.'
        });
      }
    });
  }

  reenviar(c: ConversaoDto) {
    this.servico.reenviar(c.id).subscribe({
      next: () => {
        this.toast.sucesso('De volta à fila. Sai na próxima rodada, em até um minuto.');
        this.carregar();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível reenviar.')
    });
  }

  alternarPayload(id: number) {
    this.abertaId.set(this.abertaId() === id ? null : id);
  }

  /** O nome do evento na língua da Meta — é ele que o cliente vê no Gerenciador de Eventos, e usar
   *  outro aqui faria a tela e o painel dele não conversarem. */
  nomeDoEvento(tipo: string): string {
    return tipo === 'compra' ? 'Purchase' : 'Lead';
  }

    remover() {
    // Desconectar para de mandar evento: a confirmação diz isso, porque o efeito não é visível
    // aqui — ele aparece semanas depois, na conta de anúncio, como campanha otimizando errado.
    if (!confirm('Desconectar os anúncios? O Nexora para de avisar a Meta das suas vendas.')) return;

    this.servico.remover().subscribe({
      next: () => {
        this.toast.sucesso('Anúncios desconectados.');
        this.carregar();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível desconectar.')
    });
  }
}
