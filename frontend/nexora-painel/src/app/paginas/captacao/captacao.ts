import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Canais } from '../canais/canais';
import { Formularios } from '../formularios/formularios';
import { CaptacaoServico } from '../../nucleo/servicos/captacao.servico';
import { ResumoCaptacao } from '../../nucleo/modelos';

export type AbaCaptacao = 'qr' | 'formularios';

/** CAPTAÇÃO — de onde os leads vêm.
 *
 *  ===================== POR QUE OS DOIS NUMA TELA SÓ =====================
 *  Formulário do site e QR/link respondem à MESMA pergunta do cliente, compartilham a mesma
 *  estatística e o mesmo modo de uso: criar, publicar em algum lugar, e depois olhar quantos
 *  leads vieram. Em telas separadas, comparar "o panfleto trouxe mais que a landing page?"
 *  exigia abrir duas telas e somar de cabeça.
 *
 *  ===================== O FORMULÁRIO VOLTOU, E O QR É O PADRÃO (INT-4) =====================
 *  O painel de formulários saiu da tela num bloco anterior, com uma razão boa: o cliente típico
 *  desta ferramenta não tem site nem alguém que cole HTML nele. Isso continua verdade — por isso
 *  a aba que abre é a do QR.
 *
 *  O que mudou é o que o formulário passou a valer. É ele que carrega o rastro do anúncio
 *  (`utm_*`, `fbclid`, os cookies do pixel) para dentro do CRM, e é o que permite dizer à Meta
 *  qual anúncio trouxe a venda. Sem tela, quem já tinha publicado um formulário não conseguia
 *  ver a chave, nem desligá-lo, nem pegar o código novo — a rota `/formularios` só redirecionava
 *  para cá, apesar de um comentário afirmar que ela continuava acessível.
 *  ==========================================================================================
 *
 *  ===================== O QUE ESTE COMPONENTE FAZ, E SÓ =====================
 *  Cabeçalho, resumo e abas. As duas listas continuam sendo os componentes que já existiam —
 *  eles perderam o cabeçalho de página e viraram PAINÉIS. Copiar o conteúdo deles para cá teria
 *  criado uma terceira cópia de regras que já estavam prontas e testadas.
 *  ======================================================================== */
@Component({
  selector: 'app-captacao',
  imports: [Canais, Formularios, RouterLink],
  templateUrl: './captacao.html',
  styleUrl: './captacao.css'
})
export class Captacao implements OnInit {
  private captacaoServico = inject(CaptacaoServico);
  private rota = inject(ActivatedRoute);
  private router = inject(Router);

  aba = signal<AbaCaptacao>('qr');

  // ---- o resumo
  // ===================== OS NÚMEROS SÃO DO SERVIDOR (AUD-XX) =====================
  // A tela buscava as duas listas e o resumo de anúncios e fazia todas as contas: somava os leads
  // dos formulários, contava os ativos, dividia para a fatia e decidia quando mostrar o aviso.
  // Agora uma chamada traz tudo pronto, e estes são só leituras dela.
  // =============================================================================
  private static readonly RESUMO_VAZIO: ResumoCaptacao = {
    leadsTotal: 0, leadsCanais: 0, leadsFormularios: 0,
    percentualCanais: null, percentualFormularios: null,
    canaisAtivos: 0, totalCanais: 0, formulariosAtivos: 0, totalFormularios: 0,
    leadsDeAnuncioSemEnvio: 0
  };

  resumo = signal<ResumoCaptacao>(Captacao.RESUMO_VAZIO);

  leadsCanais = computed(() => this.resumo().leadsCanais);
  canaisAtivos = computed(() => this.resumo().canaisAtivos);
  totalCanais = computed(() => this.resumo().totalCanais);
  leadsFormularios = computed(() => this.resumo().leadsFormularios);
  formulariosAtivos = computed(() => this.resumo().formulariosAtivos);
  totalFormularios = computed(() => this.resumo().totalFormularios);

  carregandoResumo = signal(true);

  /** ===================== O AVISO DE ANÚNCIO SE PERDENDO (INT-4) =====================
   *  Quantos leads dos últimos 30 dias chegaram com identificador de clique enquanto ninguém
   *  conectou a Meta.
   *
   *  ⚠️ AQUI, e não só no passo de "Primeiros passos": aquele painel some depois que o dono o
   *  fecha, e quem já é cliente há meses nunca mais o vê. Esta é a tela onde ele pensa em "de onde
   *  vêm meus leads" — é o momento em que a frase vale.
   *  ================================================================================= */
  leadsComAnuncioPerdidos = computed(() => this.resumo().leadsDeAnuncioSemEnvio);

  total = computed(() => this.resumo().leadsTotal);

  /** A fatia de cada caminho no total, de 0 a 100 e somando 100 — pronta do servidor. */
  fatiaCanais = computed(() => this.resumo().percentualCanais);
  fatiaFormularios = computed(() => this.resumo().percentualFormularios);

  /** Só formata o percentual que veio pronto. */
  pct(v: number | null): string {
    return v === null ? '—' : v.toLocaleString('pt-BR', { maximumFractionDigits: 2 });
  }

  ngOnInit() {
    // Aba pela URL: `/captacao?aba=formularios`. É o que faz o link antigo de `/formularios`
    // chegar na aba certa em vez de na primeira, e o que permite mandar "abre em Captação, aba
    // Formulário" por mensagem.
    const pedida = this.rota.snapshot.queryParamMap.get('aba');
    if (pedida === 'qr' || pedida === 'formularios') this.aba.set(pedida);

    this.carregarResumo();
  }

  trocarAba(aba: AbaCaptacao) {
    if (this.aba() === aba) return;
    this.aba.set(aba);

    // `replaceUrl`: trocar de aba não é navegação para o histórico. Sem isto, o botão "voltar"
    // do navegador percorreria as abas antes de sair da tela.
    this.router.navigate([], {
      relativeTo: this.rota,
      queryParams: { aba: aba === 'qr' ? null : aba },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  /** O resumo, numa chamada (AUD-XX). Falhar vira resumo zerado em vez de tela presa em
   *  "Carregando…", e os painéis abaixo mostram o próprio erro. */
  carregarResumo() {
    this.carregandoResumo.set(true);

    this.captacaoServico.resumo().subscribe({
      next: r => {
        this.resumo.set(r);
        this.carregandoResumo.set(false);
      },
      error: () => {
        this.resumo.set(Captacao.RESUMO_VAZIO);
        this.carregandoResumo.set(false);
      }
    });
  }
}
